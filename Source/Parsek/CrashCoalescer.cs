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
        private string cause;  // "CRASH", "OVERHEAT", "STRUCTURAL_FAILURE"

        // Pre-captured vessel snapshots at split detection time, before debris can be
        // destroyed during the coalescing window. Keyed by vessel persistentId. (#157)
        private Dictionary<uint, ConfigNode> preCapturedSnapshots = new Dictionary<uint, ConfigNode>();
        private Dictionary<uint, ConfigNode> lastEmittedSnapshots = new Dictionary<uint, ConfigNode>();

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
        public void OnSplitEvent(double ut, uint childPid, bool childHasController, string splitCause = "CRASH",
            ConfigNode preSnapshot = null)
        {
            if (!HasPendingBreakup)
            {
                // Start new coalescing window
                windowStartUT = ut;
                cause = splitCause;
                controlledChildPids.Clear();
                debrisPids.Clear();
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
                ParsekLog.Verbose("Coalescer",
                    "Debris fragment added: pid=" + childPid + " at UT=" + ut.ToString("F2", CultureInfo.InvariantCulture) +
                    " (total debris=" + debrisPids.Count + ")");
            }

            // Store pre-captured snapshot for use when coalescer emits (#157).
            // At emission time (0.5s later), the vessel may already be destroyed.
            if (preSnapshot != null && !preCapturedSnapshots.ContainsKey(childPid))
                preCapturedSnapshots[childPid] = preSnapshot;
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
                DebrisCount = debrisPids.Count,
                CoalesceWindow = coalesceWindow
            };

            // Child recording IDs will be filled in by the caller (ParsekFlight)
            // since the coalescer doesn't know recording IDs

            var ic = CultureInfo.InvariantCulture;
            ParsekLog.Info("Coalescer",
                "BREAKUP emitted: ut=" + windowStartUT.ToString("F2", ic) + " cause=" + cause +
                " controlledChildren=" + controlledChildPids.Count + " debris=" + debrisPids.Count +
                " duration=" + bp.BreakupDuration.ToString("F3", ic) + "s window=" + coalesceWindow.ToString("F1", ic) + "s");

            // Snapshot child PIDs and pre-captured snapshots before Reset clears them,
            // so the caller can access them via LastEmitted* properties.
            lastEmittedControlledChildPids.Clear();
            lastEmittedControlledChildPids.AddRange(controlledChildPids);
            lastEmittedDebrisPids.Clear();
            lastEmittedDebrisPids.AddRange(debrisPids);
            lastEmittedSnapshots.Clear();
            foreach (var kvp in preCapturedSnapshots)
                lastEmittedSnapshots[kvp.Key] = kvp.Value;

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
        /// Returns a pre-captured snapshot for the given PID, or null if none was captured.
        /// Valid immediately after Tick() returns a non-null BranchPoint. (#157)
        /// </summary>
        public ConfigNode GetPreCapturedSnapshot(uint pid)
        {
            ConfigNode snap;
            return lastEmittedSnapshots.TryGetValue(pid, out snap) ? snap : null;
        }

        /// <summary>
        /// Returns the current debris count. Only valid while HasPendingBreakup is true.
        /// </summary>
        public int CurrentDebrisCount => debrisPids.Count;

        /// <summary>
        /// Resets the coalescer, clearing all accumulated state.
        /// </summary>
        public void Reset()
        {
            windowStartUT = double.NaN;
            lastSplitUT = double.NaN;
            controlledChildPids.Clear();
            debrisPids.Clear();
            preCapturedSnapshots.Clear();
            cause = null;
            ParsekLog.Verbose("Coalescer", "Reset -- window cleared");
        }
    }
}
