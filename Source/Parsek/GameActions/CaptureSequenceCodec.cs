using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// The saved form of a capture sequence (<see cref="CaptureSequence"/>): its key and its
    /// parse. Kept apart from the issuing state so the row codec that reads and writes the
    /// value depends on nothing but this.
    /// </summary>
    internal static class CaptureSequenceCodec
    {
        internal const string Key = "cseq";

        /// <summary>A row known to have no capture number (converted from an event an older
        /// build captured): never stamped, so it gets no floor and is ahead of nothing.</summary>
        internal const long Unsequenced = -1;

        /// <summary>Parses a saved value: a positive number, <see cref="Unsequenced"/> for any
        /// negative one, 0 ("none yet") when absent or unreadable.</summary>
        internal static long Parse(string value)
        {
            long parsed;
            if (string.IsNullOrEmpty(value)
                || !long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                return 0;
            return parsed < 0 ? Unsequenced : parsed;
        }
    }
}
