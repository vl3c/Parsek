using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// Groups rapid structural split events into a single tree branch point.
    /// When the first split fires, a coalescing window opens (default 0.5s).
    /// Subsequent splits within the window are accumulated. When the window
    /// expires (checked via Tick), a single BranchPoint is emitted.
    ///
    /// <para>The emitted branch point's type depends on the split cause(s) seen during the
    /// window. A window in which <em>every</em> split was decoupler-initiated
    /// (<c>splitCause == "DECOUPLE"</c>, see
    /// <see cref="SegmentBoundaryLogic.ClassifyForegroundSplitChildCause"/>) emits a
    /// <see cref="BranchPointType.JointBreak"/> with <c>SplitCause = "DECOUPLE"</c> — an
    /// intentional staging / decoupler separation. Otherwise (any genuine collision /
    /// overstress / overheat break in the window) it emits a
    /// <see cref="BranchPointType.Breakup"/> carrying the breakup cause. This is conservative:
    /// one real breakup split in the window keeps the whole event a breakup, so a genuine
    /// failure is never relabelled a decouple.</para>
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
        // Cause to emit when the window is NOT all-decouple: "CRASH", "OVERHEAT", "STRUCTURAL_FAILURE".
        private string breakupCause;
        // True while every split accumulated so far is a decoupler-initiated separation
        // ("DECOUPLE"). Cleared by the first non-decouple split in the window.
        private bool windowAllDecouple;
        // First decoupler/root part id captured for an all-decouple window (0 if unknown);
        // surfaced as BranchPoint.DecouplerPartId on the emitted JointBreak.
        private uint windowDecouplerPartId;

        // Pre-captured vessel snapshots at split detection time, before debris can be
        // destroyed during the coalescing window. Keyed by vessel persistentId. (#157)
        private Dictionary<uint, ConfigNode> preCapturedSnapshots = new Dictionary<uint, ConfigNode>();
        private Dictionary<uint, ConfigNode> lastEmittedSnapshots = new Dictionary<uint, ConfigNode>();
        private Dictionary<uint, TrajectoryPoint> preCapturedTrajectoryPoints = new Dictionary<uint, TrajectoryPoint>();
        private Dictionary<uint, TrajectoryPoint> lastEmittedTrajectoryPoints = new Dictionary<uint, TrajectoryPoint>();

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
        /// <param name="splitCause">Cause string: "DECOUPLE" for a decoupler-initiated split,
        /// otherwise a breakup cause (CRASH, OVERHEAT, STRUCTURAL_FAILURE).</param>
        /// <param name="decouplerPartId">Root/decoupler part id for a "DECOUPLE" split (0 if unknown).</param>
        public void OnSplitEvent(double ut, uint childPid, bool childHasController, string splitCause = "CRASH",
            ConfigNode preSnapshot = null, TrajectoryPoint? preTrajectoryPoint = null, uint decouplerPartId = 0u)
        {
            bool isDecouple = string.Equals(splitCause, "DECOUPLE", StringComparison.Ordinal);
            var ic = CultureInfo.InvariantCulture;

            if (!HasPendingBreakup)
            {
                // Start new coalescing window
                windowStartUT = ut;
                windowAllDecouple = isDecouple;
                breakupCause = isDecouple ? "CRASH" : splitCause;
                windowDecouplerPartId = 0u;
                controlledChildPids.Clear();
                debrisPids.Clear();
                ParsekLog.Info("Coalescer",
                    "Coalescing window opened at UT=" + ut.ToString("F2", ic) + ", cause=" + splitCause +
                    ", isDecouple=" + isDecouple);
            }
            else if (!isDecouple)
            {
                // A genuine breakup split joined the window: the whole event becomes a
                // breakup (conservative — a real failure is never relabelled a decouple).
                if (windowAllDecouple)
                    ParsekLog.Info("Coalescer",
                        "Coalescing window escalated to breakup: non-decouple split (cause=" + splitCause +
                        ") joined an all-decouple window at UT=" + ut.ToString("F2", ic));
                windowAllDecouple = false;
                breakupCause = splitCause;
            }

            if (isDecouple && decouplerPartId != 0u && windowDecouplerPartId == 0u)
                windowDecouplerPartId = decouplerPartId;

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
            if (preTrajectoryPoint.HasValue && !preCapturedTrajectoryPoints.ContainsKey(childPid))
                preCapturedTrajectoryPoints[childPid] = preTrajectoryPoint.Value;
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

            // Window expired -- emit the branch point.
            // BreakupDuration = actual span (last split - first split),
            // not the idle window time after the last split.
            // Child recording IDs will be filled in by the caller (ParsekFlight)
            // since the coalescer doesn't know recording IDs.
            var ic = CultureInfo.InvariantCulture;
            BranchPoint bp;
            if (windowAllDecouple)
            {
                // Every split in the window was decoupler-initiated: an intentional
                // staging / decoupler separation, recorded as a JointBreak/DECOUPLE
                // (mirrors the background-recorder decouple contract).
                bp = new BranchPoint
                {
                    Id = Guid.NewGuid().ToString("N"),
                    UT = windowStartUT,
                    Type = BranchPointType.JointBreak,
                    SplitCause = "DECOUPLE",
                    DecouplerPartId = windowDecouplerPartId,
                    BreakupDuration = lastSplitUT - windowStartUT,
                    DebrisCount = debrisPids.Count,
                    CoalesceWindow = coalesceWindow
                };
                ParsekLog.Info("Coalescer",
                    "DECOUPLE emitted: ut=" + windowStartUT.ToString("F2", ic) +
                    " type=JointBreak splitCause=DECOUPLE decouplerPartId=" + windowDecouplerPartId +
                    " controlledChildren=" + controlledChildPids.Count + " debris=" + debrisPids.Count +
                    " duration=" + bp.BreakupDuration.ToString("F3", ic) + "s window=" + coalesceWindow.ToString("F1", ic) + "s");
            }
            else
            {
                bp = new BranchPoint
                {
                    Id = Guid.NewGuid().ToString("N"),
                    UT = windowStartUT,
                    Type = BranchPointType.Breakup,
                    BreakupCause = breakupCause,
                    BreakupDuration = lastSplitUT - windowStartUT,
                    DebrisCount = debrisPids.Count,
                    CoalesceWindow = coalesceWindow
                };
                ParsekLog.Info("Coalescer",
                    "BREAKUP emitted: ut=" + windowStartUT.ToString("F2", ic) + " cause=" + breakupCause +
                    " controlledChildren=" + controlledChildPids.Count + " debris=" + debrisPids.Count +
                    " duration=" + bp.BreakupDuration.ToString("F3", ic) + "s window=" + coalesceWindow.ToString("F1", ic) + "s");
            }

            // Snapshot child PIDs and pre-captured snapshots before Reset clears them,
            // so the caller can access them via LastEmitted* properties.
            lastEmittedControlledChildPids.Clear();
            lastEmittedControlledChildPids.AddRange(controlledChildPids);
            lastEmittedDebrisPids.Clear();
            lastEmittedDebrisPids.AddRange(debrisPids);
            lastEmittedSnapshots.Clear();
            foreach (var kvp in preCapturedSnapshots)
                lastEmittedSnapshots[kvp.Key] = kvp.Value;
            lastEmittedTrajectoryPoints.Clear();
            foreach (var kvp in preCapturedTrajectoryPoints)
                lastEmittedTrajectoryPoints[kvp.Key] = kvp.Value;

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
        /// Returns a pre-captured split-time trajectory point for the given PID, or null if none
        /// was captured. Valid immediately after Tick() returns a non-null BranchPoint.
        /// </summary>
        public TrajectoryPoint? GetPreCapturedTrajectoryPoint(uint pid)
        {
            TrajectoryPoint point;
            return lastEmittedTrajectoryPoints.TryGetValue(pid, out point) ? (TrajectoryPoint?)point : null;
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
            preCapturedTrajectoryPoints.Clear();
            breakupCause = null;
            windowAllDecouple = false;
            windowDecouplerPartId = 0u;
            ParsekLog.Verbose("Coalescer", "Reset -- window cleared");
        }
    }
}
