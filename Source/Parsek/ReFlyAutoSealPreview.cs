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
        DockedWithAnother,     // live vessel.situation == DOCKED -> IsHardSafetyTerminal
        Landed,                // live vessel.situation == LANDED  -> stableTerminalFocusSlot
        SplashedDown,          // live vessel.situation == SPLASHED -> stableTerminalFocusSlot
        StableOrbit,           // live vessel.situation == ORBITING && PeR > atmosphere
        SubOrbitalArc,         // live vessel.situation == SUB_ORBITAL or decaying ORBITING
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
        /// </summary>
        internal static ReFlyAutoSealPreviewResult Preview(
            Recording liveProvisional,
            ReFlySessionMarker marker,
            ParsekScenario scenario,
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
            CollectLiveVesselReasons(liveActiveVessel, reasons);

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
        ///   <item><description>Live terminal group: DockedWithAnother, Landed, SplashedDown, StableOrbit, SubOrbitalArc</description></item>
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
                case ReFlyAutoSealReason.Landed:             return 310;
                case ReFlyAutoSealReason.SplashedDown:       return 320;
                case ReFlyAutoSealReason.StableOrbit:        return 330;
                case ReFlyAutoSealReason.SubOrbitalArc:      return 340;
                default:                                     return 999;
            }
        }
    }
}
