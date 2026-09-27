using System;
using System.Globalization;

namespace Parsek
{
    internal enum TerminalOrbitSpawnSafetyAction
    {
        SpawnNow,
        DeferUntilSafe,
        CannotSpawnSafely,
    }

    internal enum TerminalOrbitDeferredSpawnState
    {
        None,

        /// <summary>A genuine deferral: wait until the recorded next safe UT.</summary>
        Hold,

        /// <summary>The deferral has expired (or has no next UT): re-evaluate the spawn now.</summary>
        Ready,

        /// <summary>
        /// A permanent CannotSpawnSafely verdict. Nothing clears it in this scene, so the ghost
        /// is not held: it ends at EndUT like any terminal that cannot spawn.
        /// </summary>
        Refused,
    }

    internal struct TerminalOrbitSpawnSafetyDecision
    {
        internal TerminalOrbitSpawnSafetyAction Action;
        internal string ReasonCode;
        internal string Reason;
        internal double CurrentAltitude;
        internal double AtmosphereDepth;
        internal double SafetyMargin;
        internal double SafeAltitude;
        internal double PeriapsisFloorAltitude;
        internal double PeriapsisAltitude;
        internal double ApoapsisAltitude;
        internal double NextSafeUT;
        internal double NextSafeAltitude;
    }

    internal static class TerminalOrbitSpawnSafety
    {
        /// <summary>
        /// Margin above the atmosphere top used ONLY by the "the vessel is low in the
        /// atmosphere band right now, wait until it climbs" deferral. It is never a periapsis
        /// line: the periapsis line is the shared <see cref="OrbitClearance"/> floor, so any
        /// orbit commit classification calls Orbiting passes this check (operator ruling
        /// 2026-09-27).
        /// </summary>
        internal const double DefaultSafetyMarginMeters = 5000.0;

        internal const string ReasonAboveSafeAltitude = "above-safe-altitude";
        internal const string ReasonCurrentAltitudeBelowSafeAltitude = "current-altitude-below-safe-altitude";
        internal const string ReasonMarginNeverReached = "periapsis-clear-margin-never-reached";
        internal const string ReasonPeriapsisBelowSafeAltitude = "periapsis-below-safe-altitude";
        internal const string ReasonNonFinitePropagatedAltitude = "non-finite-propagated-altitude";
        internal const string ReasonNonFinitePeriapsis = "non-finite-periapsis";
        internal const string ReasonNoFutureSafeUT = "no-future-safe-ut";
        internal const string ReasonSpawnedVesselDied = "spawned-terminal-orbit-vessel-died";
        internal const string ReasonTerminalOrbitResolutionFailed = "terminal-orbit-resolution-failed";

        /// <summary>
        /// Pure spawn-safety decision for a recorded terminal orbit propagated to the spawn UT.
        /// <list type="number">
        /// <item>Non-finite propagated altitude or periapsis: CannotSpawnSafely.</item>
        /// <item>Periapsis not above the periapsis floor (<see cref="OrbitClearance"/>: the
        /// atmosphere top, or the highest terrain on an airless body): CannotSpawnSafely.
        /// Commit classification reads the same floor, so a recording reaches this branch only
        /// through a verdict real play does not produce.</item>
        /// <item>Propagated altitude under the atmosphere top plus <paramref name="safetyMargin"/>
        /// while the orbit climbs above it later: DeferUntilSafe.</item>
        /// <item>Otherwise SpawnNow, including an orbit whose apoapsis never reaches the margin
        /// (it is clear of the atmosphere all the way round).</item>
        /// </list>
        /// </summary>
        internal static TerminalOrbitSpawnSafetyDecision Evaluate(
            double currentAltitude,
            double atmosphereDepth,
            double safetyMargin,
            double periapsisAltitude,
            double apoapsisAltitude,
            double maxTerrainAltitude = 0.0)
        {
            double safeAltitude = ComputeSafeAltitude(atmosphereDepth, safetyMargin);
            double periapsisFloor = OrbitClearance.ComputePeriapsisFloorAltitude(
                IsFinite(atmosphereDepth) && atmosphereDepth > 0.0,
                atmosphereDepth,
                maxTerrainAltitude);
            var inputs = new DecisionInputs
            {
                CurrentAltitude = currentAltitude,
                AtmosphereDepth = atmosphereDepth,
                SafetyMargin = safetyMargin,
                SafeAltitude = safeAltitude,
                PeriapsisFloorAltitude = periapsisFloor,
                PeriapsisAltitude = periapsisAltitude,
                ApoapsisAltitude = apoapsisAltitude,
            };

            if (!IsFinite(currentAltitude))
            {
                return BuildDecision(
                    TerminalOrbitSpawnSafetyAction.CannotSpawnSafely,
                    ReasonNonFinitePropagatedAltitude,
                    "propagated altitude is not finite",
                    inputs);
            }

            if (!IsFinite(periapsisAltitude))
            {
                return BuildDecision(
                    TerminalOrbitSpawnSafetyAction.CannotSpawnSafely,
                    ReasonNonFinitePeriapsis,
                    "terminal orbit periapsis is not finite",
                    inputs);
            }

            if (!(periapsisAltitude > periapsisFloor))
            {
                return BuildDecision(
                    TerminalOrbitSpawnSafetyAction.CannotSpawnSafely,
                    ReasonPeriapsisBelowSafeAltitude,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "terminal orbit periapsis {0:F1}m is not above the periapsis floor {1:F1}m",
                        periapsisAltitude,
                        periapsisFloor),
                    inputs);
            }

            if (currentAltitude < safeAltitude)
            {
                if (IsFinite(apoapsisAltitude) && apoapsisAltitude >= safeAltitude)
                {
                    return BuildDecision(
                        TerminalOrbitSpawnSafetyAction.DeferUntilSafe,
                        ReasonCurrentAltitudeBelowSafeAltitude,
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "propagated altitude {0:F1}m is below safe altitude {1:F1}m",
                            currentAltitude,
                            safeAltitude),
                        inputs);
                }

                return BuildDecision(
                    TerminalOrbitSpawnSafetyAction.SpawnNow,
                    ReasonMarginNeverReached,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "periapsis {0:F1}m clears the periapsis floor {1:F1}m and apoapsis {2:F1}m never " +
                        "reaches safe altitude {3:F1}m, so the orbit spawns now",
                        periapsisAltitude,
                        periapsisFloor,
                        apoapsisAltitude,
                        safeAltitude),
                    inputs);
            }

            return BuildDecision(
                TerminalOrbitSpawnSafetyAction.SpawnNow,
                ReasonAboveSafeAltitude,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "propagated altitude {0:F1}m clears safe altitude {1:F1}m and periapsis {2:F1}m clears the periapsis floor {3:F1}m",
                    currentAltitude,
                    safeAltitude,
                    periapsisAltitude,
                    periapsisFloor),
                inputs);
        }

        private struct DecisionInputs
        {
            internal double CurrentAltitude;
            internal double AtmosphereDepth;
            internal double SafetyMargin;
            internal double SafeAltitude;
            internal double PeriapsisFloorAltitude;
            internal double PeriapsisAltitude;
            internal double ApoapsisAltitude;
        }

        internal static bool ShouldHoldDeferredSpawnUntilUT(
            Recording rec,
            double currentUT,
            out string reason)
        {
            return GetDeferredSpawnState(rec, currentUT, out reason) == TerminalOrbitDeferredSpawnState.Hold;
        }

        internal static TerminalOrbitDeferredSpawnState GetDeferredSpawnState(
            Recording rec,
            double currentUT,
            out string reason)
        {
            reason = null;
            if (rec == null)
                return TerminalOrbitDeferredSpawnState.None;

            if (rec.TerminalSpawnCannotSpawnSafely)
            {
                reason = rec.TerminalSpawnSafetyReasonCode ?? ReasonPeriapsisBelowSafeAltitude;
                return TerminalOrbitDeferredSpawnState.Refused;
            }

            if (!rec.TerminalSpawnSafetyDeferred)
                return TerminalOrbitDeferredSpawnState.None;

            // A deferral always carries a finite next attempt UT (MarkDeferred is only called
            // with one). Without one there is nothing to wait for, so the spawn is re-evaluated
            // now instead of holding forever.
            if (!IsFinite(rec.TerminalSpawnNextAttemptUT))
            {
                reason = (rec.TerminalSpawnSafetyReasonCode ?? ReasonCurrentAltitudeBelowSafeAltitude)
                    + "; nextUT=non-finite";
                return TerminalOrbitDeferredSpawnState.Ready;
            }

            if (!IsFinite(currentUT) || currentUT < rec.TerminalSpawnNextAttemptUT)
            {
                reason = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}; nextUT={1:R} currentUT={2:R}",
                    rec.TerminalSpawnSafetyReasonCode ?? ReasonCurrentAltitudeBelowSafeAltitude,
                    rec.TerminalSpawnNextAttemptUT,
                    currentUT);
                return TerminalOrbitDeferredSpawnState.Hold;
            }

            return TerminalOrbitDeferredSpawnState.Ready;
        }

        internal static void MarkDeferred(
            Recording rec,
            TerminalOrbitSpawnSafetyDecision decision,
            double decisionUT,
            double nextAttemptUT,
            double pressure)
        {
            if (rec == null)
                return;

            rec.TerminalSpawnSafetyDeferred = true;
            rec.TerminalSpawnCannotSpawnSafely = false;
            rec.TerminalSpawnSafetyReasonCode = decision.ReasonCode;
            rec.TerminalSpawnSafetyReason = decision.Reason;
            rec.TerminalSpawnSafetyDecisionUT = decisionUT;
            rec.TerminalSpawnNextAttemptUT = nextAttemptUT;
            rec.TerminalSpawnSafetyAltitude = decision.CurrentAltitude;
            rec.TerminalSpawnSafetySafeAltitude = decision.SafeAltitude;
            rec.TerminalSpawnSafetyPeriapsisAltitude = decision.PeriapsisAltitude;
            rec.TerminalSpawnSafetyApoapsisAltitude = decision.ApoapsisAltitude;
            rec.TerminalSpawnSafetyPressure = pressure;
        }

        internal static void MarkCannotSpawnSafely(
            Recording rec,
            TerminalOrbitSpawnSafetyDecision decision,
            double decisionUT,
            double pressure)
        {
            if (rec == null)
                return;

            rec.TerminalSpawnSafetyDeferred = false;
            rec.TerminalSpawnCannotSpawnSafely = true;
            rec.TerminalSpawnSafetyReasonCode = decision.ReasonCode;
            rec.TerminalSpawnSafetyReason = decision.Reason;
            rec.TerminalSpawnSafetyDecisionUT = decisionUT;
            rec.TerminalSpawnNextAttemptUT = double.NaN;
            rec.TerminalSpawnSafetyAltitude = decision.CurrentAltitude;
            rec.TerminalSpawnSafetySafeAltitude = decision.SafeAltitude;
            rec.TerminalSpawnSafetyPeriapsisAltitude = decision.PeriapsisAltitude;
            rec.TerminalSpawnSafetyApoapsisAltitude = decision.ApoapsisAltitude;
            rec.TerminalSpawnSafetyPressure = pressure;
        }

        internal static void Clear(Recording rec)
        {
            if (rec == null)
                return;

            rec.TerminalSpawnSafetyDeferred = false;
            rec.TerminalSpawnCannotSpawnSafely = false;
            rec.TerminalSpawnSafetyReasonCode = null;
            rec.TerminalSpawnSafetyReason = null;
            rec.TerminalSpawnSafetyDecisionUT = double.NaN;
            rec.TerminalSpawnNextAttemptUT = double.NaN;
            rec.TerminalSpawnSafetyAltitude = double.NaN;
            rec.TerminalSpawnSafetySafeAltitude = double.NaN;
            rec.TerminalSpawnSafetyPeriapsisAltitude = double.NaN;
            rec.TerminalSpawnSafetyApoapsisAltitude = double.NaN;
            rec.TerminalSpawnSafetyPressure = double.NaN;
        }

        internal static bool HasActiveHold(Recording rec)
        {
            return rec != null
                && (rec.TerminalSpawnSafetyDeferred || rec.TerminalSpawnCannotSpawnSafely);
        }

        internal static double ComputeSafeAltitude(double atmosphereDepth, double safetyMargin)
        {
            double depth = IsFinite(atmosphereDepth) && atmosphereDepth > 0.0
                ? atmosphereDepth
                : 0.0;
            double margin = depth > 0.0 && IsFinite(safetyMargin) && safetyMargin > 0.0
                ? safetyMargin
                : 0.0;
            return depth + margin;
        }

        internal static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static TerminalOrbitSpawnSafetyDecision BuildDecision(
            TerminalOrbitSpawnSafetyAction action,
            string reasonCode,
            string reason,
            DecisionInputs inputs)
        {
            return new TerminalOrbitSpawnSafetyDecision
            {
                Action = action,
                ReasonCode = reasonCode,
                Reason = reason,
                CurrentAltitude = inputs.CurrentAltitude,
                AtmosphereDepth = inputs.AtmosphereDepth,
                SafetyMargin = inputs.SafetyMargin,
                SafeAltitude = inputs.SafeAltitude,
                PeriapsisFloorAltitude = inputs.PeriapsisFloorAltitude,
                PeriapsisAltitude = inputs.PeriapsisAltitude,
                ApoapsisAltitude = inputs.ApoapsisAltitude,
                NextSafeUT = double.NaN,
                NextSafeAltitude = double.NaN,
            };
        }
    }
}
