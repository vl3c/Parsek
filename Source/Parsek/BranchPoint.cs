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
        Breakup    = 5
    }

    public class BranchPoint
    {
        public string Id;
        public double UT;
        public BranchPointType Type;
        public List<string> ParentRecordingIds = new List<string>();
        public List<string> ChildRecordingIds = new List<string>();

        // Breakup-specific metadata (only used when Type == Breakup)
        public string BreakupCause;       // "CRASH", "OVERHEAT", "STRUCTURAL_FAILURE"
        public double BreakupDuration;    // seconds from first to last split in window
        public int DebrisCount;           // number of uncontrolled debris fragments
        public double CoalesceWindow;     // coalescing window duration in seconds

        public override string ToString()
        {
            if (Type == BranchPointType.Breakup)
                return $"BP id={Id ?? "?"} type={Type} ut={UT:F1} " +
                       $"parents={ParentRecordingIds.Count} children={ChildRecordingIds.Count} " +
                       $"cause={BreakupCause ?? "?"} debris={DebrisCount} duration={BreakupDuration:F3}s";

            return $"BP id={Id ?? "?"} type={Type} ut={UT:F1} " +
                   $"parents={ParentRecordingIds.Count} children={ChildRecordingIds.Count}";
        }
    }
}
