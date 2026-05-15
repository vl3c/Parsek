using System;
using System.Collections.Generic;
using System.Text;

namespace Parsek
{
    /// <summary>
    /// Pre-transition merge dialog (Re-Fly variant) preview of whether the
    /// merge will trigger auto-seal of the slot, and the player-attributable
    /// reason(s). Mirrors a subset of the production classifier in
    /// <see cref="SupersedeCommit"/>: detects the cases that are reliable
    /// from live state (Ledger.Actions for science, tree topology for
    /// structural mutations, live <see cref="Vessel.Situations"/> for
    /// stable terminals).
    ///
    /// <para>Read-only and conservative: false negatives over false positives.
    /// When the preview cannot determine seal, the dialog falls back to the
    /// default "permanently to the timeline" copy. The production classifier
    /// runs at finalize and seals correctly regardless of what the preview
    /// said.</para>
    /// </summary>
    internal enum ReFlyAutoSealReason
    {
        EarnedScience,         // any retry-blocking ScienceEarning row in the lineage
        TransmittedScience,    // ScienceMethod.Transmitted
        RecoveredScience,      // ScienceMethod.Recovered
        Undocked,              // BranchPointType.Undock
        KerbalEva,             // BranchPointType.EVA
        PartBrokeOff,          // BranchPointType.JointBreak
        VesselBrokeUp,         // BranchPointType.Breakup
        DockedWithAnother,     // DOCKED situation (live) or TerminalState.Docked (recorded) -> IsHardSafetyTerminal
        VesselRecovered,       // TerminalState.Recovered -> IsHardSafetyTerminal
        Landed,                // LANDED situation (live) or TerminalState.Landed (recorded) -> stableTerminalFocusSlot
        SplashedDown,          // SPLASHED situation (live) or TerminalState.Splashed (recorded) -> stableTerminalFocusSlot
        StableOrbit,           // ORBITING situation && PeR > atmosphere (live) or TerminalState.Orbiting (recorded) -> stableTerminalFocusSlot
        SubOrbitalArc,         // SUB_ORBITAL situation (live) or TerminalState.SubOrbital (recorded) -> stableTerminalFocusSlot
    }

    /// <summary>
    /// Result of <see cref="ReFlyAutoSealPreviewer.Preview"/>.
    /// <see cref="WillAutoSeal"/> is true when at least one reason was
    /// detected. <see cref="Reasons"/> is sorted by group (science ->
    /// structural -> terminal) for stable test output and player
    /// readability.
    /// </summary>
    internal struct ReFlyAutoSealPreviewResult
    {
        public bool WillAutoSeal;
        public List<ReFlyAutoSealReason> Reasons;

        public static ReFlyAutoSealPreviewResult NoSeal()
        {
            return new ReFlyAutoSealPreviewResult
            {
                WillAutoSeal = false,
                Reasons = new List<ReFlyAutoSealReason>(0),
            };
        }

        /// <summary>
        /// Composes a player-facing phrase from <see cref="Reasons"/>.
        /// Subject-free phrasing (e.g. "transmitted science and undocked")
        /// for the dialog body's "for the following reason(s):" wrapping.
        ///
        /// <list type="bullet">
        ///   <item><description>0 reasons: returns null. Caller shows default copy.</description></item>
        ///   <item><description>1 reason: bare phrase, e.g. "transmitted science".</description></item>
        ///   <item><description>2 reasons: "{a} and {b}".</description></item>
        ///   <item><description>3+ reasons: "{a}, {b}, ..., and {n}" (Oxford comma).</description></item>
        /// </list>
        /// </summary>
        public string FormatHumanReadable()
        {
            if (Reasons == null || Reasons.Count == 0)
                return null;

            if (Reasons.Count == 1)
                return PhraseFor(Reasons[0]);

            if (Reasons.Count == 2)
                return PhraseFor(Reasons[0]) + " and " + PhraseFor(Reasons[1]);

            var sb = new StringBuilder();
            for (int i = 0; i < Reasons.Count; i++)
            {
                if (i > 0)
                {
                    if (i == Reasons.Count - 1)
                        sb.Append(", and ");
                    else
                        sb.Append(", ");
                }
                sb.Append(PhraseFor(Reasons[i]));
            }
            return sb.ToString();
        }

        internal static string PhraseFor(ReFlyAutoSealReason reason)
        {
            switch (reason)
            {
                case ReFlyAutoSealReason.EarnedScience:      return "earned science";
                case ReFlyAutoSealReason.TransmittedScience: return "transmitted science";
                case ReFlyAutoSealReason.RecoveredScience:   return "recovered science";
                case ReFlyAutoSealReason.Undocked:           return "undocked";
                case ReFlyAutoSealReason.KerbalEva:          return "sent a kerbal on EVA";
                case ReFlyAutoSealReason.PartBrokeOff:       return "broke off a part";
                case ReFlyAutoSealReason.VesselBrokeUp:      return "the vessel broke up";
                case ReFlyAutoSealReason.DockedWithAnother:  return "docked with another vessel";
                case ReFlyAutoSealReason.VesselRecovered:    return "the vessel was recovered";
                case ReFlyAutoSealReason.Landed:             return "landed";
                case ReFlyAutoSealReason.SplashedDown:       return "splashed down";
                case ReFlyAutoSealReason.StableOrbit:        return "reached a stable orbit";
                case ReFlyAutoSealReason.SubOrbitalArc:      return "reached a sub-orbital arc";
                default:                                     return reason.ToString();
            }
        }
    }

    internal static class ReFlyAutoSealPreviewer
    {
        /// <summary>
        /// Compute the auto-seal preview. Read-only: no Ledger / scenario /
        /// state-version mutation. Returns <see cref="ReFlyAutoSealPreviewResult.NoSeal"/>
        /// on null guards or TreeId mismatch.
        ///
        /// <para>The structural-mutation gate resolves its rewind-point
        /// cutoff through <c>ParsekScenario.Instance</c> internally; no
        /// scenario parameter is needed here. Tests set the singleton via
        /// <c>ParsekScenario.SetInstanceForTesting</c>.</para>
        /// </summary>
        internal static ReFlyAutoSealPreviewResult Preview(
            Recording liveProvisional,
            ReFlySessionMarker marker,
            Vessel liveActiveVessel)
        {
            // Null guards mirror the production gate's early-returns at
            // SupersedeCommit.HasReFlySessionStructuralMutation:683-688.
            if (marker == null) return ReFlyAutoSealPreviewResult.NoSeal();
            if (liveProvisional == null) return ReFlyAutoSealPreviewResult.NoSeal();
            if (string.IsNullOrEmpty(marker.TreeId)) return ReFlyAutoSealPreviewResult.NoSeal();
            if (string.IsNullOrEmpty(liveProvisional.TreeId))
                return ReFlyAutoSealPreviewResult.NoSeal();
            if (!string.Equals(liveProvisional.TreeId, marker.TreeId,
                    StringComparison.Ordinal))
                return ReFlyAutoSealPreviewResult.NoSeal();

            var reasons = new List<ReFlyAutoSealReason>();

            // Earned-science check mirrors SupersedeCommit's
            // IsRetryBlockingRecordingAction (:1257-1290) which calls
            // IsWorldStateChangingRecordingAction (:1231-1255). Today only
            // ScienceEarning rows are retry-blocking, but other action
            // types are still walked through the same exclusion gates so
            // future retry-blocking action types pick up automatically.
            HashSet<string> lineageRecordingIds =
                SupersedeCommit.CollectRecordingIdsForSafetyGate(liveProvisional);
            CollectScienceReasons(lineageRecordingIds, reasons);

            // Structural-mutation check via the typed accessor extracted
            // alongside HasReFlySessionStructuralMutation.
            if (SupersedeCommit.TryGetFirstReFlySessionStructuralMutationType(
                    liveProvisional, marker, out BranchPointType firstType))
            {
                ReFlyAutoSealReason? mapped = MapStructuralBranchPoint(firstType);
                if (mapped.HasValue) AddIfMissing(reasons, mapped.Value);
            }

            // Live vessel terminal proxy. Sampled at dialog spawn only;
            // KSP physics may continue in background while the dialog is
            // open under LockInput, so the situation could in theory flip
            // before the player clicks. Acceptable as a snapshot - the
            // production classifier re-classifies at finalize.
            //
            // Re-Fly sessions can vessel-switch (background a recording,
            // promote another vessel to active) while the marker still
            // identifies the original slot, so FlightGlobals.ActiveVessel
            // may be a different vessel than the one liveProvisional
            // describes. Surfacing live-terminal reasons from an unrelated
            // active vessel would mislead the player; the production
            // classifier runs against the Re-Fly recording, not the live
            // active vessel. Gate the live-vessel branch by persistent id:
            // skip when the active vessel does not match the recording's
            // VesselPersistentId.
            Vessel reFlyVessel =
                IsLiveVesselReFlyTarget(liveActiveVessel, liveProvisional)
                    ? liveActiveVessel
                    : null;
            CollectLiveVesselReasons(reFlyVessel, reasons);

            // Recorded-terminal fallback. Production seals on the recording's
            // own terminal via stableTerminalFocusSlot (Orbiting / SubOrbital
            // / Landed / Splashed; UnfinishedFlightClassifier:241) and
            // stableTerminal + IsHardSafetyTerminal (Recovered / Docked;
            // SupersedeCommit:1017-1019, 1052-1062). The deferred merge
            // fallback (ShowTreeDialog's 1-arg overload, fired in Space
            // Center / Tracking Station / re-entered flight when the
            // pre-transition dialog was missed) cannot rely on
            // FlightGlobals.ActiveVessel for terminal signal, so derive the
            // reason from the finalized recording too. In pre-transition the
            // recording is still being produced so TerminalStateValue is
            // typically null - the live-vessel proxy above is the source.
            // Duplicates de-dup via AddIfMissing.
            CollectRecordedTerminalReasons(liveProvisional, reasons);

            SortReasonsByGroup(reasons);

            return new ReFlyAutoSealPreviewResult
            {
                WillAutoSeal = reasons.Count > 0,
                Reasons = reasons,
            };
        }

        private static void CollectScienceReasons(
            HashSet<string> lineageRecordingIds,
            List<ReFlyAutoSealReason> reasons)
        {
            if (lineageRecordingIds == null || lineageRecordingIds.Count == 0)
                return;
            var actions = Ledger.Actions;
            if (actions == null) return;

            bool sawTransmitted = false;
            bool sawRecovered = false;
            bool sawUnknownMethod = false;
            for (int i = 0; i < actions.Count; i++)
            {
                var action = actions[i];
                if (action == null) continue;
                if (action.Type != GameActionType.ScienceEarning) continue;
                if (string.IsNullOrEmpty(action.RecordingId)) continue;
                if (!lineageRecordingIds.Contains(action.RecordingId)) continue;

                // Mirror the production filter: skip tombstone-eligible
                // (e.g. paired with kerbal death penalty) actions.
                if (TombstoneEligibility.IsEligible(action)) continue;
                if (TombstoneEligibility.TryPairBundledRepPenalty(
                        action, actions, out _))
                    continue;

                if (action.Method == ScienceMethod.Transmitted)
                    sawTransmitted = true;
                else if (action.Method == ScienceMethod.Recovered)
                    sawRecovered = true;
                else
                    sawUnknownMethod = true;
            }

            if (sawTransmitted)
                AddIfMissing(reasons, ReFlyAutoSealReason.TransmittedScience);
            if (sawRecovered)
                AddIfMissing(reasons, ReFlyAutoSealReason.RecoveredScience);
            if (sawUnknownMethod)
                AddIfMissing(reasons, ReFlyAutoSealReason.EarnedScience);
        }

        private static bool IsLiveVesselReFlyTarget(
            Vessel candidate, Recording reFlyRec)
        {
            if (candidate == null) return false;
            if (reFlyRec == null) return false;
            return ShouldUseLiveVesselForReFlyTarget(
                candidate.persistentId, reFlyRec.VesselPersistentId);
        }

        /// <summary>
        /// Pure persistent-id comparison extracted for unit-testability.
        /// The live-vessel terminal branch should run only when the
        /// candidate vessel's pid matches the Re-Fly recording's pid AND
        /// neither is the zero sentinel. Internal so tests can pin the
        /// matching contract without faking a <see cref="Vessel"/>.
        /// </summary>
        internal static bool ShouldUseLiveVesselForReFlyTarget(
            uint candidatePid, uint expectedPid)
        {
            if (candidatePid == 0) return false;
            if (expectedPid == 0) return false;
            return candidatePid == expectedPid;
        }

        private static ReFlyAutoSealReason? MapStructuralBranchPoint(
            BranchPointType type)
        {
            switch (type)
            {
                case BranchPointType.Undock:     return ReFlyAutoSealReason.Undocked;
                case BranchPointType.EVA:        return ReFlyAutoSealReason.KerbalEva;
                case BranchPointType.JointBreak: return ReFlyAutoSealReason.PartBrokeOff;
                case BranchPointType.Breakup:    return ReFlyAutoSealReason.VesselBrokeUp;
                default:                         return null;
            }
        }

        private static void CollectLiveVesselReasons(
            Vessel liveActiveVessel,
            List<ReFlyAutoSealReason> reasons)
        {
            if (liveActiveVessel == null) return;
            switch (liveActiveVessel.situation)
            {
                case Vessel.Situations.DOCKED:
                    AddIfMissing(reasons, ReFlyAutoSealReason.DockedWithAnother);
                    break;
                case Vessel.Situations.LANDED:
                    AddIfMissing(reasons, ReFlyAutoSealReason.Landed);
                    break;
                case Vessel.Situations.SPLASHED:
                    AddIfMissing(reasons, ReFlyAutoSealReason.SplashedDown);
                    break;
                case Vessel.Situations.SUB_ORBITAL:
                    AddIfMissing(reasons, ReFlyAutoSealReason.SubOrbitalArc);
                    break;
                case Vessel.Situations.ORBITING:
                    // Defensive null-check (mirrors RecordingTree.cs:863):
                    // vessel.orbit and vessel.orbit.referenceBody can be null
                    // in transient pre-launch / scene-switch frames. Treat
                    // as FLYING in that case (no live-terminal reason).
                    Orbit orbit = liveActiveVessel.orbit;
                    if (orbit == null) return;
                    CelestialBody body = orbit.referenceBody;
                    if (body == null) return;
                    bool stable = RecordingTree.IsBoundOrbitAboveAtmosphere(
                        orbit.eccentricity,
                        orbit.PeR,
                        body.Radius,
                        body.atmosphere,
                        body.atmosphereDepth);
                    AddIfMissing(reasons, stable
                        ? ReFlyAutoSealReason.StableOrbit
                        : ReFlyAutoSealReason.SubOrbitalArc);
                    break;
                // PRELAUNCH / FLYING / ESCAPING: no stable terminal yet.
            }
        }

        private static void CollectRecordedTerminalReasons(
            Recording reFlyRec,
            List<ReFlyAutoSealReason> reasons)
        {
            if (reFlyRec == null) return;
            TerminalState? terminal = reFlyRec.TerminalStateValue;
            if (!terminal.HasValue) return;
            switch (terminal.Value)
            {
                case TerminalState.Landed:
                    AddIfMissing(reasons, ReFlyAutoSealReason.Landed);
                    break;
                case TerminalState.Splashed:
                    AddIfMissing(reasons, ReFlyAutoSealReason.SplashedDown);
                    break;
                case TerminalState.Orbiting:
                    AddIfMissing(reasons, ReFlyAutoSealReason.StableOrbit);
                    break;
                case TerminalState.SubOrbital:
                    AddIfMissing(reasons, ReFlyAutoSealReason.SubOrbitalArc);
                    break;
                case TerminalState.Docked:
                    AddIfMissing(reasons, ReFlyAutoSealReason.DockedWithAnother);
                    break;
                case TerminalState.Recovered:
                    AddIfMissing(reasons, ReFlyAutoSealReason.VesselRecovered);
                    break;
                // Destroyed: returns "crashed" earlier in the classifier (no
                // seal here, the Re-Fly retry-on-crash flow takes over).
                // Boarded: kerbal EVA recordings ending Boarded almost
                // always share a structural KerbalEva reason via the parent
                // tree's BranchPointType.EVA; the structural classifier
                // covers that user-facing warning. A pure-Boarded recording
                // with no upstream EVA structural row is rare and left to
                // the production classifier's authoritative seal verdict.
            }
        }

        private static void AddIfMissing(
            List<ReFlyAutoSealReason> reasons, ReFlyAutoSealReason reason)
        {
            for (int i = 0; i < reasons.Count; i++)
                if (reasons[i] == reason) return;
            reasons.Add(reason);
        }

        /// <summary>
        /// Stable readability ordering. Production picks the first close-reason
        /// hit and stops; the preview is intentionally a superset that lists all
        /// player-attributable reasons in a deterministic order so tests are
        /// stable and the player gets the full picture.
        ///
        /// <list type="number">
        ///   <item><description>Science group: TransmittedScience, RecoveredScience, EarnedScience</description></item>
        ///   <item><description>Structural group: Undocked, KerbalEva, PartBrokeOff, VesselBrokeUp</description></item>
        ///   <item><description>Terminal group: DockedWithAnother, VesselRecovered, Landed, SplashedDown, StableOrbit, SubOrbitalArc</description></item>
        /// </list>
        /// </summary>
        private static void SortReasonsByGroup(List<ReFlyAutoSealReason> reasons)
        {
            reasons.Sort((a, b) => GroupOrdinal(a).CompareTo(GroupOrdinal(b)));
        }

        private static int GroupOrdinal(ReFlyAutoSealReason reason)
        {
            switch (reason)
            {
                case ReFlyAutoSealReason.TransmittedScience: return 100;
                case ReFlyAutoSealReason.RecoveredScience:   return 110;
                case ReFlyAutoSealReason.EarnedScience:      return 120;
                case ReFlyAutoSealReason.Undocked:           return 200;
                case ReFlyAutoSealReason.KerbalEva:          return 210;
                case ReFlyAutoSealReason.PartBrokeOff:       return 220;
                case ReFlyAutoSealReason.VesselBrokeUp:      return 230;
                case ReFlyAutoSealReason.DockedWithAnother:  return 300;
                case ReFlyAutoSealReason.VesselRecovered:    return 305;
                case ReFlyAutoSealReason.Landed:             return 310;
                case ReFlyAutoSealReason.SplashedDown:       return 320;
                case ReFlyAutoSealReason.StableOrbit:        return 330;
                case ReFlyAutoSealReason.SubOrbitalArc:      return 340;
                default:                                     return 999;
            }
        }
    }
}
