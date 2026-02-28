using System.Collections.Generic;

namespace Parsek
{
    public enum BranchPointType
    {
        Undock = 0,
        EVA    = 1,
        Dock   = 2,
        Board  = 3
    }

    public class BranchPoint
    {
        public string id;
        public double ut;
        public BranchPointType type;
        public List<string> parentRecordingIds = new List<string>();
        public List<string> childRecordingIds = new List<string>();

        public override string ToString()
        {
            return $"BP id={id ?? "?"} type={type} ut={ut:F1} " +
                   $"parents={parentRecordingIds.Count} children={childRecordingIds.Count}";
        }
    }
}
