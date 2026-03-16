using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// Groups rapid structural split events into a single BREAKUP tree event.
    /// When the first split fires, a coalescing window opens (default 0.5s).
    /// Subsequent splits within the window are accumulated. When the window
    /// expires (checked via Tick), a single BREAKUP BranchPoint is emitted.
    /// </summary>
    internal class CrashCoalescer
    {
        internal const double DefaultCoalesceWindow = 0.5;  // seconds

        private double windowStartUT = double.NaN;
        private double coalesceWindow;

        // Accumulated split data during the window
        private List<uint> controlledChildPids = new List<uint>();
        private int debrisCount;
        private string cause;  // "CRASH", "OVERHEAT", "STRUCTURAL_FAILURE"

        public bool HasPendingBreakup => !double.IsNaN(windowStartUT);
        public double WindowStartUT => windowStartUT;

        public CrashCoalescer(double window = DefaultCoalesceWindow)
        {
            coalesceWindow = window;
        }

        /// <summary>
        /// Called when a structural split event fires. Starts or extends the
        /// coalescing window.
        /// </summary>
        /// <param name="ut">Universal time of the split</param>
        /// <param name="childPid">PersistentId of the child vessel</param>
        /// <param name="childHasController">Whether the child has a command part</param>
        /// <param name="splitCause">Cause string (CRASH, OVERHEAT, STRUCTURAL_FAILURE)</param>
        public void OnSplitEvent(double ut, uint childPid, bool childHasController, string splitCause = "CRASH")
        {
            if (!HasPendingBreakup)
            {
                // Start new coalescing window
                windowStartUT = ut;
                cause = splitCause;
                controlledChildPids.Clear();
                debrisCount = 0;
                var ic = CultureInfo.InvariantCulture;
                ParsekLog.Info("CrashCoalescer",
                    "Coalescing window opened at UT=" + ut.ToString("F2", ic) + ", cause=" + splitCause);
            }

            if (childHasController)
            {
                controlledChildPids.Add(childPid);
                ParsekLog.Verbose("CrashCoalescer",
                    "Controlled child added: pid=" + childPid + " at UT=" + ut.ToString("F2", CultureInfo.InvariantCulture) +
                    " (total controlled=" + controlledChildPids.Count + ")");
            }
            else
            {
                debrisCount++;
                ParsekLog.Verbose("CrashCoalescer",
                    "Debris fragment added at UT=" + ut.ToString("F2", CultureInfo.InvariantCulture) +
                    " (total debris=" + debrisCount + ")");
            }
        }

        /// <summary>
        /// Called each frame to check if the coalescing window has expired.
        /// Returns a BREAKUP BranchPoint when the window expires, null otherwise.
        /// </summary>
        public BranchPoint Tick(double currentUT)
        {
            if (!HasPendingBreakup)
                return null;

            if (currentUT - windowStartUT < coalesceWindow)
                return null;

            // Window expired -- emit BREAKUP event
            var bp = new BranchPoint
            {
                Id = Guid.NewGuid().ToString("N"),
                UT = windowStartUT,
                Type = BranchPointType.Breakup,
                BreakupCause = cause,
                BreakupDuration = currentUT - windowStartUT,
                DebrisCount = debrisCount,
                CoalesceWindow = coalesceWindow
            };

            // Child recording IDs will be filled in by the caller (ParsekFlight)
            // since the coalescer doesn't know recording IDs

            var ic = CultureInfo.InvariantCulture;
            ParsekLog.Info("CrashCoalescer",
                "BREAKUP emitted: ut=" + windowStartUT.ToString("F2", ic) + " cause=" + cause +
                " controlledChildren=" + controlledChildPids.Count + " debris=" + debrisCount +
                " duration=" + bp.BreakupDuration.ToString("F3", ic) + "s window=" + coalesceWindow.ToString("F1", ic) + "s");

            Reset();
            return bp;
        }

        /// <summary>
        /// Returns the list of controlled child vessel PIDs accumulated during
        /// the current window. Only valid while HasPendingBreakup is true.
        /// </summary>
        public IReadOnlyList<uint> ControlledChildPids => controlledChildPids;

        /// <summary>
        /// Returns the current debris count. Only valid while HasPendingBreakup is true.
        /// </summary>
        public int CurrentDebrisCount => debrisCount;

        /// <summary>
        /// Resets the coalescer, clearing all accumulated state.
        /// </summary>
        public void Reset()
        {
            windowStartUT = double.NaN;
            controlledChildPids.Clear();
            debrisCount = 0;
            cause = null;
            ParsekLog.Verbose("CrashCoalescer", "Reset -- window cleared");
        }
    }
}
