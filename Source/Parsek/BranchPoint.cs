using System.Collections.Generic;

namespace Parsek
{
    public enum BranchPointType
    {
        Undock     = 0,
        EVA        = 1,
        Dock       = 2,
        Board      = 3,
        JointBreak = 4
    }

    public class BranchPoint
    {
        public string Id;
        public double UT;
        public BranchPointType Type;
        public List<string> ParentRecordingIds = new List<string>();
        public List<string> ChildRecordingIds = new List<string>();

        public override string ToString()
        {
            return $"BP id={Id ?? "?"} type={Type} ut={UT:F1} " +
                   $"parents={ParentRecordingIds.Count} children={ChildRecordingIds.Count}";
        }
    }
}
