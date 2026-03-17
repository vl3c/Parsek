using System.Collections.Generic;

namespace Parsek
{
    public enum BranchPointType
    {
        Undock     = 0,
        EVA        = 1,
        Dock       = 2,
        Board      = 3,
        JointBreak = 4,
        Launch     = 5,
        Breakup    = 6,
        Terminal   = 7
    }

    public class BranchPoint
    {
        public string Id;
        public double UT;
        public BranchPointType Type;
        public List<string> ParentRecordingIds = new List<string>();
        public List<string> ChildRecordingIds = new List<string>();

        // SPLIT metadata (for Undock, EVA, JointBreak)
        public string SplitCause;              // "DECOUPLE", "UNDOCK", "EVA" (null if not applicable)
        public uint DecouplerPartId;           // Part that triggered separation (0 if not applicable)

        // BREAKUP metadata (for Breakup type)
        public string BreakupCause;            // "CRASH", "OVERHEAT", "STRUCTURAL_FAILURE"
        public double BreakupDuration;         // Time window of the breakup
        public int DebrisCount;                // Number of non-tracked debris fragments
        public double CoalesceWindow;          // Time threshold used for grouping (default 0.5s)

        // MERGE metadata (for Dock, Board)
        public string MergeCause;              // "DOCK", "BOARD", "CONSTRUCT", "CLAW"
        public uint TargetVesselPersistentId;  // Pre-existing vessel if applicable (0 if not)

        // TERMINAL metadata (for Terminal type)
        public string TerminalCause;           // "RECOVERED", "DESTROYED", "RECYCLED", "DESPAWNED"

        public override string ToString()
        {
            var s = $"BP id={Id ?? "?"} type={Type} ut={UT:F1} " +
                    $"parents={ParentRecordingIds.Count} children={ChildRecordingIds.Count}";
            if (Type == BranchPointType.Breakup)
                s += $" cause={BreakupCause ?? "?"} debris={DebrisCount} duration={BreakupDuration:F3}s";
            else if (Type == BranchPointType.Terminal && TerminalCause != null)
                s += $" terminal={TerminalCause}";
            else if (SplitCause != null)
                s += $" splitCause={SplitCause}";
            else if (MergeCause != null)
                s += $" mergeCause={MergeCause}";
            return s;
        }
    }
}
