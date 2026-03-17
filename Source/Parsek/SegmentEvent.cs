using System.Globalization;

namespace Parsek
{
    public enum SegmentEventType
    {
        ControllerChange  = 0,  // controlling part changed (e.g. probe core switched)
        ControllerDisabled = 1, // controller lost (destroyed, no power, etc.)
        ControllerEnabled = 2,  // controller regained
        CrewLost          = 3,  // crew member lost (death, EVA exit handled elsewhere)
        CrewTransfer      = 4,  // crew member moved between parts
        PartDestroyed     = 5,  // part destroyed while vessel stays connected
        PartRemoved       = 6,  // part removed (e.g. inventory removal) without split
        PartAdded         = 7   // part added (e.g. inventory placement) without merge
    }

    public struct SegmentEvent
    {
        public double ut;
        public SegmentEventType type;
        public string details;  // Type-specific data. Null means no details (not empty string).

        public override string ToString()
        {
            return string.Format(CultureInfo.InvariantCulture,
                "SegmentEvent type={0} ut={1:F2} details={2}", type, ut, details ?? "none");
        }
    }
}
