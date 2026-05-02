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
        Hold,
        Ready,
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
        internal double PeriapsisAltitude;
        internal double ApoapsisAltitude;
        internal double NextSafeUT;
        internal double NextSafeAltitude;
    }

    internal static class TerminalOrbitSpawnSafety
    {
        internal const double DefaultSafetyMarginMeters = 5000.0;

        internal const string ReasonAboveSafeAltitude = "above-safe-altitude";
        internal const string ReasonCurrentAltitudeBelowSafeAltitude = "current-altitude-below-safe-altitude";
        internal const string ReasonOrbitNeverClearsSafeAltitude = "orbit-never-clears-safe-altitude";
        internal const string ReasonPeriapsisBelowSafeAltitude = "periapsis-below-safe-altitude";
        internal const string ReasonNonFinitePropagatedAltitude = "non-finite-propagated-altitude";
        internal const string ReasonNonFinitePeriapsis = "non-finite-periapsis";
        internal const string ReasonNoFutureSafeUT = "no-future-safe-ut";
        internal const string ReasonSpawnedVesselDied = "spawned-terminal-orbit-vessel-died";

        internal static TerminalOrbitSpawnSafetyDecision Evaluate(
            double currentAltitude,
            double atmosphereDepth,
            double safetyMargin,
            double periapsisAltitude,
            double apoapsisAltitude)
        {
            double safeAltitude = ComputeSafeAltitude(atmosphereDepth, safetyMargin);

            if (!IsFinite(currentAltitude))
            {
                return BuildDecision(
                    TerminalOrbitSpawnSafetyAction.CannotSpawnSafely,
                    ReasonNonFinitePropagatedAltitude,
                    "propagated altitude is not finite",
                    currentAltitude,
                    atmosphereDepth,
                    safetyMargin,
                    safeAltitude,
                    periapsisAltitude,
                    apoapsisAltitude);
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
                        currentAltitude,
                        atmosphereDepth,
                        safetyMargin,
                        safeAltitude,
                        periapsisAltitude,
                        apoapsisAltitude);
                }

                return BuildDecision(
                    TerminalOrbitSpawnSafetyAction.CannotSpawnSafely,
                    ReasonOrbitNeverClearsSafeAltitude,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "propagated altitude {0:F1}m is below safe altitude {1:F1}m and apoapsis {2:F1}m does not clear it",
                        currentAltitude,
                        safeAltitude,
                        apoapsisAltitude),
                    currentAltitude,
                    atmosphereDepth,
                    safetyMargin,
                    safeAltitude,
                    periapsisAltitude,
                    apoapsisAltitude);
            }

            if (!IsFinite(periapsisAltitude))
            {
                return BuildDecision(
                    TerminalOrbitSpawnSafetyAction.CannotSpawnSafely,
                    ReasonNonFinitePeriapsis,
                    "terminal orbit periapsis is not finite",
                    currentAltitude,
                    atmosphereDepth,
                    safetyMargin,
                    safeAltitude,
                    periapsisAltitude,
                    apoapsisAltitude);
            }

            if (periapsisAltitude < safeAltitude)
            {
                return BuildDecision(
                    TerminalOrbitSpawnSafetyAction.CannotSpawnSafely,
                    ReasonPeriapsisBelowSafeAltitude,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "terminal orbit periapsis {0:F1}m is below safe altitude {1:F1}m",
                        periapsisAltitude,
                        safeAltitude),
                    currentAltitude,
                    atmosphereDepth,
                    safetyMargin,
                    safeAltitude,
                    periapsisAltitude,
                    apoapsisAltitude);
            }

            return BuildDecision(
                TerminalOrbitSpawnSafetyAction.SpawnNow,
                ReasonAboveSafeAltitude,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "propagated altitude {0:F1}m and periapsis {1:F1}m clear safe altitude {2:F1}m",
                    currentAltitude,
                    periapsisAltitude,
                    safeAltitude),
                currentAltitude,
                atmosphereDepth,
                safetyMargin,
                safeAltitude,
                periapsisAltitude,
                apoapsisAltitude);
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
                return TerminalOrbitDeferredSpawnState.Hold;
            }

            if (!rec.TerminalSpawnSafetyDeferred)
                return TerminalOrbitDeferredSpawnState.None;

            if (!IsFinite(rec.TerminalSpawnNextAttemptUT))
            {
                reason = rec.TerminalSpawnSafetyReasonCode ?? ReasonCurrentAltitudeBelowSafeAltitude;
                return TerminalOrbitDeferredSpawnState.Hold;
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
            double margin = IsFinite(safetyMargin) && safetyMargin > 0.0
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
            double currentAltitude,
            double atmosphereDepth,
            double safetyMargin,
            double safeAltitude,
            double periapsisAltitude,
            double apoapsisAltitude)
        {
            return new TerminalOrbitSpawnSafetyDecision
            {
                Action = action,
                ReasonCode = reasonCode,
                Reason = reason,
                CurrentAltitude = currentAltitude,
                AtmosphereDepth = atmosphereDepth,
                SafetyMargin = safetyMargin,
                SafeAltitude = safeAltitude,
                PeriapsisAltitude = periapsisAltitude,
                ApoapsisAltitude = apoapsisAltitude,
                NextSafeUT = double.NaN,
                NextSafeAltitude = double.NaN,
            };
        }
    }
}
