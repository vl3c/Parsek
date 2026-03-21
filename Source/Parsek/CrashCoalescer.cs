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
        private double lastSplitUT = double.NaN;
        private double coalesceWindow;

        // Accumulated split data during the window
        private List<uint> controlledChildPids = new List<uint>();
        private List<uint> debrisPids = new List<uint>();
        private int debrisCount;
        private string cause;  // "CRASH", "OVERHEAT", "STRUCTURAL_FAILURE"

        // Snapshot of child PIDs from the last emitted BREAKUP,
        // preserved across Reset() so the caller can access them after Tick() returns.
        private List<uint> lastEmittedControlledChildPids = new List<uint>();
        private List<uint> lastEmittedDebrisPids = new List<uint>();

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
                debrisPids.Clear();
                debrisCount = 0;
                var ic = CultureInfo.InvariantCulture;
                ParsekLog.Info("Coalescer",
                    "Coalescing window opened at UT=" + ut.ToString("F2", ic) + ", cause=" + splitCause);
            }

            lastSplitUT = ut;

            if (childHasController)
            {
                controlledChildPids.Add(childPid);
                ParsekLog.Verbose("Coalescer",
                    "Controlled child added: pid=" + childPid + " at UT=" + ut.ToString("F2", CultureInfo.InvariantCulture) +
                    " (total controlled=" + controlledChildPids.Count + ")");
            }
            else
            {
                debrisPids.Add(childPid);
                debrisCount++;
                ParsekLog.Verbose("Coalescer",
                    "Debris fragment added: pid=" + childPid + " at UT=" + ut.ToString("F2", CultureInfo.InvariantCulture) +
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
            // BreakupDuration = actual breakup span (last split - first split),
            // not the idle window time after the last split.
            var bp = new BranchPoint
            {
                Id = Guid.NewGuid().ToString("N"),
                UT = windowStartUT,
                Type = BranchPointType.Breakup,
                BreakupCause = cause,
                BreakupDuration = lastSplitUT - windowStartUT,
                DebrisCount = debrisCount,
                CoalesceWindow = coalesceWindow
            };

            // Child recording IDs will be filled in by the caller (ParsekFlight)
            // since the coalescer doesn't know recording IDs

            var ic = CultureInfo.InvariantCulture;
            ParsekLog.Info("Coalescer",
                "BREAKUP emitted: ut=" + windowStartUT.ToString("F2", ic) + " cause=" + cause +
                " controlledChildren=" + controlledChildPids.Count + " debris=" + debrisCount +
                " duration=" + bp.BreakupDuration.ToString("F3", ic) + "s window=" + coalesceWindow.ToString("F1", ic) + "s");

            // Snapshot child PIDs before Reset clears them,
            // so the caller can access them via LastEmittedControlledChildPids / LastEmittedDebrisPids.
            lastEmittedControlledChildPids.Clear();
            lastEmittedControlledChildPids.AddRange(controlledChildPids);
            lastEmittedDebrisPids.Clear();
            lastEmittedDebrisPids.AddRange(debrisPids);

            Reset();
            return bp;
        }

        /// <summary>
        /// Returns the list of controlled child vessel PIDs accumulated during
        /// the current window. Only valid while HasPendingBreakup is true.
        /// </summary>
        public IReadOnlyList<uint> ControlledChildPids => controlledChildPids;

        /// <summary>
        /// Returns the controlled child PIDs from the last emitted BREAKUP.
        /// Valid immediately after Tick() returns a non-null BranchPoint.
        /// Overwritten on the next Tick() emission. Not cleared by Reset()
        /// (Reset only clears the active window; this snapshot persists for the caller).
        /// </summary>
        public IReadOnlyList<uint> LastEmittedControlledChildPids => lastEmittedControlledChildPids;

        /// <summary>
        /// Returns the debris child PIDs from the last emitted BREAKUP.
        /// Same lifecycle as LastEmittedControlledChildPids.
        /// </summary>
        public IReadOnlyList<uint> LastEmittedDebrisPids => lastEmittedDebrisPids;

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
            lastSplitUT = double.NaN;
            controlledChildPids.Clear();
            debrisPids.Clear();
            debrisCount = 0;
            cause = null;
            ParsekLog.Verbose("Coalescer", "Reset -- window cleared");
        }
    }
}
